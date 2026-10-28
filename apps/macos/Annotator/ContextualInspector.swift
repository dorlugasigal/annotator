import AnnotationCore
import SwiftUI

/// A tool's or selection's properties. Beside a vertical toolbar they stack in a column (`compact`); everywhere else
/// they stay on one row, and the toolbar grows sideways to fit them.
struct ContextualInspector: View {
    @ObservedObject var model: WorkspaceModel
    var compact = false
    private var tool: Tool { model.effectiveTool }
    private var fillVisible: Bool { (model.style.fill?.alpha ?? 0) > 0 }
    private var propertyLayout: AnyLayout {
        compact ? AnyLayout(VStackLayout(alignment: .leading, spacing: 14)) :
            AnyLayout(HStackLayout(alignment: .propertyControl, spacing: 14))
    }
    private var rootLayout: AnyLayout {
        compact ? AnyLayout(VStackLayout(alignment: .leading, spacing: 10)) :
            AnyLayout(HStackLayout(alignment: .propertyControl, spacing: 14))
    }

    var body: some View {
        rootLayout {
            if model.selectionCount > 0 {
                Label(
                    model.selectionIsWorkspaceWide ? "Editing \(model.selectionCount) objects across displays" :
                        model.selectionCount == 1 ? "Editing \(tool.label)" : "Editing \(model.selectionCount) selected objects",
                    systemImage: "square.dashed"
                )
                .font(.system(size: 11, weight: .semibold))
                .fixedSize()
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
                        HStack(spacing: 6) {
                            Slider(
                                value: Binding(get: { model.style.opacity }, set: { value in model.applyStyle { $0.opacity = value } }),
                                in: 0...1, onEditingChanged: styleEditing
                            ).frame(width: 180)
                            Text("\(Int(model.style.opacity * 100))%").font(.caption.monospacedDigit())
                        }
                        Text("Drag handles to resize or rotate. Delete removes the selected image.").font(.caption).foregroundStyle(
                            .secondary)
                    }
                }
            } else if tool.isProtection {
                propertyLayout {
                    CoverPropertiesView(model: model, compact: compact)
                    Text("Blurred details can still be readable. Remove private info before you share.")
                        .font(.caption).foregroundStyle(.secondary)
                        .fixedSize(horizontal: !compact, vertical: true)
                }
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
                    HStack(alignment: .propertyControl, spacing: 10) {
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
                            CycleOptions(
                                name: "Fill style",
                                values: FillPattern.allCases.map { ($0, $0.rawValue.capitalized, .fill($0)) },
                                selection: model.style.fillPattern
                            ) { value in model.applyStyle { $0.fillPattern = value } }
                        }.fixedSize(horizontal: true, vertical: false)
                    }
                    if tool == .rectangle || tool == .diamond {
                        PropertyBlock(name: "Edges", symbol: "rectangle") {
                            InspectorToggle(name: "Rounded corners", sample: .edges(true), isOn: model.style.cornerRadius > 0,
                                            onHelp: "Rounded corners are on. Click for sharp corners.",
                                            offHelp: "Sharp corners. Click to round them.") {
                                let rounded = model.style.cornerRadius > 0
                                model.applyStyle { $0.cornerRadius = rounded ? 0 : 14 }
                            }
                        }.fixedSize(horizontal: true, vertical: false)
                    }
                    if tool == .grid {
                        HStack {
                            StyleValueControl(model: model, parameter: .gridRows, sliderWidth: 0)
                            StyleValueControl(model: model, parameter: .gridColumns, sliderWidth: 0)
                        }
                    }
                    if tool == .axes {
                        HStack(spacing: 12) {
                            Toggle(
                                "Centered axes",
                                isOn: Binding(
                                    get: { model.style.negativeAxes ?? false },
                                    set: { value in model.applyStyle { $0.negativeAxes = value } })
                            ).font(.caption)
                                .help("Centered axes also go in the negative directions.")
                                .accessibilityLabel("Centered axes with negative directions")
                            StyleValueControl(model: model, parameter: .axisTickCount, sliderWidth: 0)
                                .help(model.style.axisTickCount == nil
                                    ? "Legacy tick spacing. Set a count for equal intervals from the origin; 0 hides ticks."
                                    : "Evenly spaced in each direction. More ticks reduce spacing; 0 hides ticks.")
                        }
                        .fixedSize()
                    }
                    if tool == .pen {
                        PropertyBlock(name: "Pressure", symbol: "pencil.tip") {
                            InspectorToggle(name: "Ink pressure", sample: .pressure(true), isOn: model.style.pressure,
                                            onHelp: "Ink pressure is on: lines get thinner and thicker. Click for one width.",
                                            offHelp: "One width all along. Click for ink pressure.") {
                                let pressure = model.style.pressure
                                model.applyStyle { $0.pressure = !pressure }
                            }
                        }.fixedSize(horizontal: true, vertical: false)
                        PropertyBlock(name: "Smart Draw", symbol: "wand.and.stars") {
                            SmartDrawButton(model: model)
                        }.fixedSize(horizontal: true, vertical: false)
                    }
                    // The arrow's choices follow Roughness on the same row; a column puts them after the others.
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
            StyleValueControl(model: model, parameter: .width, sliderWidth: 96, showLabel: false, showsField: false)
                .frame(height: 36)
        }.fixedSize(horizontal: true, vertical: false)
    }
    private var arrowControls: some View {
            propertyLayout {
                PropertyBlock(name: "Start", symbol: "arrow.left") {
                    ArrowEndControl(model: model, start: true)
                }.fixedSize(horizontal: true, vertical: false)
                PropertyBlock(name: "End", symbol: "arrow.right") {
                    ArrowEndControl(model: model, start: false)
                }.fixedSize(horizontal: true, vertical: false)
                PropertyBlock(name: "Curved", symbol: "arrow.turn.up.right") {
                    InspectorToggle(name: "Curved arrow", sample: .route(true), isOn: model.style.curved,
                                    onHelp: "Curved arrow is on. Click for a straight arrow.",
                                    offHelp: "Straight arrow. Click to curve it.") {
                        let curved = model.style.curved
                        model.applyStyle { $0.curved = !curved }
                    }
                }.fixedSize(horizontal: true, vertical: false)
                PropertyBlock(name: "Head size", symbol: "arrow.up.left.and.arrow.down.right") {
                    HStack(spacing: 6) {
                        Slider(value: Binding(get: { model.style.headScale ?? 1 }, set: { value in model.applyStyle { $0.headScale = value } }),
                               in: 0.5...2, onEditingChanged: styleEditing).frame(width: 86).accessibilityLabel("Arrowhead size")
                        Text("\(Int((model.style.headScale ?? 1) * 100))%").font(.caption.monospacedDigit())
                            .foregroundStyle(.secondary).frame(minWidth: 34, alignment: .leading)
                    }.frame(height: 36)
                }.fixedSize(horizontal: true, vertical: false)
            }
            .accessibilityElement(children: .contain).accessibilityLabel("Arrow properties")
    }
    private var patternControls: some View {
        PropertyBlock(name: "Pattern", symbol: "line.3.horizontal") {
            CycleOptions(
                name: "Pattern",
                values: StrokePattern.allCases.map { ($0, $0.rawValue.capitalized, .pattern($0)) },
                selection: model.style.pattern
            ) { value in model.applyStyle { $0.pattern = value } }
        }.fixedSize(horizontal: true, vertical: false)
    }
    private var roughnessControls: some View {
        PropertyBlock(name: "Roughness", symbol: "scribble") {
            CycleOptions(
                name: "Roughness",
                values: [(0.0, "Clean", .roughness(0)), (2.0, "Sketchy", .roughness(2)), (6.0, "Wild", .roughness(6))],
                selection: model.style.roughness
            ) { value in model.applyStyle { $0.roughness = value } }
        }.fixedSize(horizontal: true, vertical: false)
    }
    private var textControls: some View {
        propertyLayout {
            InspectorColorPicker(model: model)
            PropertyBlock(name: "Font", symbol: "textformat") {
                CycleOptions(
                    name: "Font",
                    choices: [("system", "System", .text("Aa", .default)), ("monospace", "Mono", .text("Aa", .monospaced))],
                    selection: model.style.fontName == "monospace" ? "monospace" : "system"
                ) { name in model.applyStyle { $0.fontName = name } }
            }.fixedSize(horizontal: true, vertical: false)
            PropertyBlock(name: "Size", symbol: "textformat.size") {
                StyleValueControl(model: model, parameter: .textSize, sliderWidth: 96, showLabel: false, showsField: false)
                    .frame(height: 36)
            }.fixedSize(horizontal: true, vertical: false)
            PropertyBlock(name: "Weight", symbol: "bold") {
                InspectorControlButton(name: "Bold", selected: model.style.bold, action: { model.applyStyle { $0.bold.toggle() } }) {
                    Image(systemName: "bold").font(.system(size: 17))
                }.accessibilityValue(model.style.bold ? "On" : "Off")
            }.fixedSize(horizontal: true, vertical: false)
            PropertyBlock(name: "Alignment", symbol: "text.alignleft") {
                CycleOptions(
                    name: "Alignment",
                    choices: AnnotationCore.TextAlignment.allCases.map { alignment in
                        (alignment, alignment.rawValue.capitalized, .symbol(
                            alignment == .left ? "text.alignleft" : alignment == .center ? "text.aligncenter" : "text.alignright"))
                    },
                    selection: model.style.alignment
                ) { alignment in model.applyStyle { $0.alignment = alignment } }
            }.fixedSize(horizontal: true, vertical: false)
        }
    }
    private func styleEditing(_ editing: Bool) {
        if editing { model.beginStyleEdit() } else { model.finishStyleEdit() }
    }
}
