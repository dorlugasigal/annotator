import AnnotationCore
import SwiftUI

struct ContextualInspector: View {
    @ObservedObject var model: WorkspaceModel
    var compact = false
    private var tool: Tool { model.effectiveTool }
    private var fillVisible: Bool { (model.style.fill?.alpha ?? 0) > 0 }
    private var propertyLayout: AnyLayout {
        compact ? AnyLayout(VStackLayout(alignment: .leading, spacing: 14)) : AnyLayout(AdaptivePropertyLayout())
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            if model.selectionCount > 0 {
                Label(
                    model.selectionIsWorkspaceWide ? "Editing \(model.selectionCount) objects across displays" :
                        model.selectionCount == 1 ? "Editing \(tool.label)" : "Editing \(model.selectionCount) selected objects",
                    systemImage: "square.dashed"
                )
                .font(.system(size: 11, weight: .semibold))
                .accessibilityIdentifier("selected-object-context")
            }
            if tool == .select && model.selectionCount > 0 {
                propertyLayout {
                    InspectorColorPicker(model: model)
                    if model.selectionSupportsFill { InspectorColorPicker(model: model, fill: true) }
                    widthControls
                    StyleValueControl(model: model, parameter: .opacity, sliderWidth: compact ? 64 : 90)
                }
            } else if tool == .select {
                Text("Select an object to edit its properties.").font(.caption).foregroundStyle(.secondary)
            } else if tool == .image {
                PropertyBlock(name: "Image opacity", symbol: "photo") {
                    propertyLayout {
                        Slider(
                            value: Binding(get: { model.style.opacity }, set: { value in model.applyStyle { $0.opacity = value } }),
                            in: 0...1, onEditingChanged: styleEditing
                        ).frame(width: 180)
                        Text("\(Int(model.style.opacity * 100))%").font(.caption.monospacedDigit())
                        Text("Drag handles to resize or rotate. Delete removes the selected image.").font(.caption).foregroundStyle(
                            .secondary)
                    }
                }
            } else if tool.isProtection {
                CoverPropertiesView(model: model, compact: compact)
                Text("Blur, pixelation and distortion can leave details readable. Remove private information before sharing.")
                    .font(.caption).foregroundStyle(.secondary)
            } else if tool == .counter || tool == .ruler {
                propertyLayout {
                    InspectorColorPicker(model: model)
                    if tool == .ruler { widthControls }
                    StyleValueControl(model: model, parameter: .opacity, sliderWidth: compact ? 64 : 90)
                    Text(tool == .counter ? "Click to add sequential numbered markers." : "Drag to measure the distance in source pixels.")
                        .font(.caption).foregroundStyle(.secondary)
                }
            } else if tool == .eraser {
                widthControls
            } else if tool == .cover {
                HStack {
                    Label("Solid censor. Select it and press Delete to remove it.", systemImage: "eye.slash")
                        .font(.system(size: 11)).foregroundStyle(.secondary)
                    if model.selectionCount > 0 { Button("Delete censor") { model.deleteSelected() } }
                }
            } else if tool == .text {
                textControls
            } else {
                propertyLayout {
                    HStack(alignment: .top, spacing: 10) {
                        InspectorColorPicker(model: model)
                        if tool.supportsFill { InspectorColorPicker(model: model, fill: true) }
                    }
                    widthControls
                    if tool != .highlighter {
                        patternControls
                        roughnessControls
                    }
                    if tool.supportsFill && fillVisible {
                        PropertyBlock(name: "Fill style", symbol: "square.lefthalf.filled") {
                            SampleOptions(
                                values: FillPattern.allCases.map { ($0, $0.rawValue.capitalized, .fill($0)) },
                                selection: model.style.fillPattern
                            ) { value in model.applyStyle { $0.fillPattern = value } }
                        }
                    }
                    if tool == .rectangle || tool == .diamond {
                        PropertyBlock(name: "Edges", symbol: "rectangle") {
                            SampleOptions(
                                values: [(false, "Sharp corners", .edges(false)), (true, "Rounded corners", .edges(true))],
                                selection: model.style.cornerRadius > 0
                            ) { rounded in
                                model.applyStyle { $0.cornerRadius = rounded ? 14 : 0 }
                            }
                        }
                    }
                    if tool == .grid {
                        HStack {
                            StyleValueControl(model: model, parameter: .gridRows, sliderWidth: 0)
                            StyleValueControl(model: model, parameter: .gridColumns, sliderWidth: 0)
                        }
                    }
                    if tool == .axes {
                        VStack(alignment: .leading, spacing: 8) {
                            Toggle(
                                "Centered axes with negative directions",
                                isOn: Binding(
                                    get: { model.style.negativeAxes ?? false },
                                    set: { value in model.applyStyle { $0.negativeAxes = value } })
                            ).font(.caption)
                            StyleValueControl(model: model, parameter: .axisTickCount, sliderWidth: 0)
                            Text(
                                model.style.axisTickCount == nil
                                    ? "Legacy tick spacing. Set a count for equal intervals from the origin; 0 hides ticks."
                                    : "Evenly spaced in each direction. More ticks reduce spacing; 0 hides ticks."
                            )
                            .font(.caption).foregroundStyle(.secondary)
                        }
                    }
                    if tool == .pen {
                        PropertyBlock(name: "Pressure", symbol: "pencil.tip") {
                            HStack(spacing: 16) {
                                pressureChoice(false, label: "Constant")
                                pressureChoice(true, label: "Tablet")
                            }
                        }.fixedSize(horizontal: true, vertical: false)
                        PropertyBlock(name: "Smart Draw", symbol: "wand.and.stars") {
                            SmartDrawButton(model: model)
                        }.fixedSize(horizontal: true, vertical: false)
                    }
                    // The arrow's choices follow Roughness on the same line when the toolbar has room.
                    if tool == .arrow && !compact { arrowControls }
                }
            }
            if tool == .arrow && compact { arrowControls }
        }
        .controlSize(.small)
        .disabled(model.selectionStyleIsLocked)
        .transaction { $0.animation = nil }
    }

    private var widthControls: some View {
        PropertyBlock(name: tool == .eraser ? "Eraser size" : "Width", symbol: "lineweight") {
            SampleOptions(
                values: (tool == .eraser ? [8.0, 16, 28] : [2.0, 4, 7]).map { ($0, "Width \($0.formatted())", .thickness($0)) },
                selection: model.style.width
            ) { value in model.applyStyle { $0.width = value } }
            StyleValueControl(model: model, parameter: .width, sliderWidth: 80, showLabel: false)
        }
    }
    private var arrowControls: some View {
            propertyLayout {
                PropertyBlock(name: "Start", symbol: "arrow.left") {
                    ArrowEndControl(model: model, start: true)
                }.fixedSize(horizontal: true, vertical: false)
                PropertyBlock(name: "End", symbol: "arrow.right") {
                    ArrowEndControl(model: model, start: false)
                }.fixedSize(horizontal: true, vertical: false)
                PropertyBlock(name: "Arrow type", symbol: "arrow.up.right") {
                    HStack(spacing: 7) {
                        ForEach([false, true], id: \.self) { curved in
                            VStack(spacing: 4) {
                                SampleButton(name: curved ? "Curved arrow" : "Straight arrow",
                                             sample: .route(curved), selected: model.style.curved == curved) {
                                    model.applyStyle { $0.curved = curved }
                                }
                                Text(curved ? "Curved" : "Straight").font(.system(size: 10)).foregroundStyle(.secondary)
                            }
                        }
                    }
                }.fixedSize(horizontal: true, vertical: false)
                PropertyBlock(name: "Head size", symbol: "arrow.up.left.and.arrow.down.right") {
                    SampleOptions(values: [0.5, 0.75, 1].map { ($0, "\(Int($0 * 100))% arrowhead", .headSize($0)) },
                                  selection: model.style.headScale ?? 1) { value in model.applyStyle { $0.headScale = value } }
                    HStack(spacing: 6) {
                        Slider(value: Binding(get: { model.style.headScale ?? 1 }, set: { value in model.applyStyle { $0.headScale = value } }),
                               in: 0.5...2, onEditingChanged: styleEditing).frame(width: 86).accessibilityLabel("Arrowhead size")
                        Text("\(Int((model.style.headScale ?? 1) * 100))%").font(.system(size: 10).monospacedDigit()).frame(width: 32, alignment: .trailing)
                    }
                }.fixedSize(horizontal: true, vertical: false)
            }
            .accessibilityElement(children: .contain).accessibilityLabel("Arrow properties")
    }
    private var patternControls: some View {
        PropertyBlock(name: "Pattern", symbol: "line.3.horizontal") {
            SampleOptions(
                values: StrokePattern.allCases.map { ($0, $0.rawValue.capitalized, .pattern($0)) },
                selection: model.style.pattern
            ) { value in model.applyStyle { $0.pattern = value } }
        }
    }
    private var roughnessControls: some View {
        PropertyBlock(name: "Roughness", symbol: "scribble") {
            SampleOptions(
                values: [(0.0, "Clean", .roughness(0)), (2.0, "Sketchy", .roughness(2)), (6.0, "Wild", .roughness(6))],
                selection: model.style.roughness
            ) { value in model.applyStyle { $0.roughness = value } }
        }
    }
    private func pressureChoice(_ pressure: Bool, label: String) -> some View {
        VStack(spacing: 4) {
            SampleButton(name: label + " pressure", sample: .pressure(pressure), selected: model.style.pressure == pressure) {
                model.applyStyle { $0.pressure = pressure }
            }
            Text(label).font(.system(size: 10)).foregroundStyle(.secondary)
        }
    }
    private var textControls: some View {
        propertyLayout {
            InspectorColorPicker(model: model)
            PropertyBlock(name: "Font", symbol: "textformat") {
                HStack(spacing: 5) {
                    fontChoice("System", name: "system", design: .default)
                    fontChoice("Mono", name: "monospace", design: .monospaced)
                }
            }
            PropertyBlock(name: "Size", symbol: "textformat.size") {
                HStack(spacing: 4) {
                    ForEach([18.0, 28, 44], id: \.self) { size in
                        InspectorControlButton(name: "Font size \(Int(size))", selected: model.style.fontSize == size, action: {
                            model.applyStyle { $0.fontSize = size }
                        }) {
                            Text("A").font(.system(size: size / 2))
                        }
                    }
                }
                StyleValueControl(model: model, parameter: .textSize, sliderWidth: 80, showLabel: false)
            }
            PropertyBlock(name: "Weight", symbol: "bold") {
                InspectorControlButton(name: "Bold", selected: model.style.bold, action: { model.applyStyle { $0.bold.toggle() } }) {
                    Image(systemName: "bold").font(.system(size: 17))
                }.accessibilityValue(model.style.bold ? "On" : "Off")
            }
            PropertyBlock(name: "Alignment", symbol: "text.alignleft") {
                HStack(spacing: 5) {
                    ForEach(AnnotationCore.TextAlignment.allCases, id: \.self) { alignment in
                        InspectorControlButton(name: "Align \(alignment.rawValue)", selected: model.style.alignment == alignment, action: {
                            model.applyStyle { $0.alignment = alignment }
                        }) {
                            Image(
                                systemName: alignment == .left
                                    ? "text.alignleft" : alignment == .center ? "text.aligncenter" : "text.alignright"
                            )
                        }
                    }
                }
            }
        }
    }
    private func fontChoice(_ title: String, name: String, design: Font.Design) -> some View {
        InspectorControlButton(name: "\(title) font", selected: model.style.fontName == name, action: {
            model.applyStyle { $0.fontName = name }
        }) {
            Text("Aa").font(.system(size: 17, design: design))
        }
    }
    private func styleEditing(_ editing: Bool) {
        if editing { model.beginStyleEdit() } else { model.finishStyleEdit() }
    }
}
