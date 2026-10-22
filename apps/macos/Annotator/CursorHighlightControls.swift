import SwiftUI
import AnnotationCore
import PresentationRendering

struct CursorHighlightControls: View {
    @ObservedObject var model: WorkspaceModel
    @State private var colorsExpanded = false
    private var settings: CursorHighlightSettings { model.cursorHighlightSettings }
    private let colors: [RGBA] = [
        RGBA(1, 0.82, 0), RGBA(0.08, 0.8, 0.84), .blue, RGBA(0.75, 0.36, 0.93),
        RGBA(0.98, 0.21, 0.51), .coral, RGBA(0.27, 0.76, 0.34), .white
    ]

    private func binding<Value>(_ key: WritableKeyPath<CursorHighlightSettings, Value>) -> Binding<Value> {
        Binding(get: { settings[keyPath: key] }, set: { value in
            var changed = settings
            changed[keyPath: key] = value
            model.setCursorHighlight(changed)
        })
    }

    private func chooseColor(_ color: RGBA) {
        var changed = settings
        changed.color = RGBA(color.red, color.green, color.blue, settings.color.alpha)
        model.setCursorHighlight(changed)
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                HStack(alignment: .firstTextBaseline) {
                    VStack(alignment: .leading, spacing: 6) {
                        Text("Follow my lead.").font(.title2.bold())
                        Text("Easy to spot. Easy on the eyes.").foregroundStyle(.secondary)
                    }
                    Spacer()
                    Toggle("Show halo", isOn: $model.cursorHalo).toggleStyle(.switch)
                        .help("Show or hide the highlight around your real pointer.")
                }
                HStack(alignment: .top, spacing: 24) {
                    VStack(alignment: .leading, spacing: 16) {
                        Text("Shape").font(.headline)
                        LazyVGrid(columns: Array(repeating: GridItem(.flexible(minimum: 42), spacing: 8), count: 4), spacing: 8) {
                            ForEach(CursorHighlightSettings.Shape.allCases, id: \.self) { shape in
                                Button {
                                    var changed = settings; changed.shape = shape
                                    model.setCursorHighlight(changed)
                                } label: {
                                    Group {
                                        if shape == .disc { HaloShapeIcon(shape: shape).fill() }
                                        else { HaloShapeIcon(shape: shape)
                                            .strokeBorder(style: StrokeStyle(lineWidth: 2, lineCap: .round, lineJoin: .round)) }
                                    }.frame(width: 25, height: 25).padding(12)
                                        .frame(maxWidth: .infinity)
                                        .background(settings.shape == shape ? Color.accentColor.opacity(0.18) : Color.primary.opacity(0.045),
                                                    in: RoundedRectangle(cornerRadius: 8))
                                }.buttonStyle(.plain).help(shape.label).accessibilityLabel(shape.label)
                                    .accessibilityAddTraits(settings.shape == shape ? .isSelected : [])
                            }
                        }
                        Picker("Outline", selection: Binding(get: { settings.resolvedBorder }, set: {
                            var changed = settings; changed.borderStyle = $0; model.setCursorHighlight(changed)
                        })) {
                            Text("Solid").tag(StrokePattern.solid)
                            Text("Dashed").tag(StrokePattern.dashed)
                            Text("Dotted").tag(StrokePattern.dotted)
                        }.disabled(settings.shape == .disc)
                        HStack(spacing: 8) {
                            ForEach(colors.indices, id: \.self) { index in
                                Button { chooseColor(colors[index]) } label: {
                                    Circle().fill(Color(nsColor: colors[index].nsColor)).frame(width: 23, height: 23)
                                        .overlay { Circle().strokeBorder(Color.primary.opacity(0.2)) }
                                }.buttonStyle(.plain)
                                    .accessibilityLabel(["Yellow", "Cyan", "Blue", "Purple", "Pink", "Coral", "Green", "White"][index])
                            }
                        }
                        Button { colorsExpanded.toggle() } label: {
                            Label("Custom color", systemImage: colorsExpanded ? "chevron.down" : "chevron.right")
                        }.buttonStyle(.plain).accessibilityValue(colorsExpanded ? "Expanded" : "Collapsed")
                        if colorsExpanded {
                            InlineColorWheel(color: settings.color, choose: chooseColor)
                                .frame(maxWidth: 270)
                        }
                    }.frame(maxWidth: .infinity, alignment: .leading)
                    VStack(spacing: 10) {
                        CursorHighlightPreview(settings: settings)
                            .frame(width: 200, height: 200)
                            .clipShape(RoundedRectangle(cornerRadius: 16))
                            .overlay { RoundedRectangle(cornerRadius: 16).strokeBorder(Color.primary.opacity(0.08)) }
                        Text("Move here. Give it a click.").font(.callout.weight(.medium))
                        Text("This preview won't draw on your screen.")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                }
                Divider()
                VStack(spacing: 12) {
                    slider("Size", value: binding(\.diameter), range: 16...160, suffix: " pt")
                    slider("Border", value: binding(\.borderWidth), range: 1...12, suffix: " pt")
                        .disabled(settings.shape == .disc)
                    slider("Opacity", value: Binding(get: { settings.color.alpha * 100 }, set: {
                        var changed = settings; changed.color.alpha = $0/100; model.setCursorHighlight(changed)
                    }), range: 0...100, suffix: "%")
                    slider("Glow", value: Binding(get: { settings.resolvedGlow }, set: {
                        var changed = settings; changed.glowRadius = $0; model.setCursorHighlight(changed)
                    }), range: 0...24, suffix: " pt")
                }
                Divider()
                Picker("On click", selection: Binding(get: { settings.resolvedClickEffect }, set: {
                    var changed = settings
                    changed.clickEffect = $0; changed.showClicks = $0 != .off
                    model.setCursorHighlight(changed)
                })) {
                    ForEach(CursorHighlightSettings.ClickEffect.allCases, id: \.self) { effect in
                        Text(effect.label).tag(effect)
                    }
                }.pickerStyle(.segmented)
                Picker("Show highlight", selection: binding(\.visibility)) {
                    Text("While turned on").tag(CursorHighlightSettings.Visibility.always)
                    Text("When I move").tag(CursorHighlightSettings.Visibility.moving)
                    Text("When I click").tag(CursorHighlightSettings.Visibility.clicks)
                }
                .help("Clicks animate once, then settle. Reduce Motion uses a quiet, static emphasis.")
            }.padding(24)
        }
        .background(Color(nsColor: .windowBackgroundColor))
    }

    private func slider(_ label: String, value: Binding<Double>, range: ClosedRange<Double>, suffix: String) -> some View {
        HStack {
            Text(label).font(.callout).frame(width: 65, alignment: .leading)
            Slider(value: value, in: range).accessibilityLabel("Cursor \(label.lowercased())")
            Text("\(Int(value.wrappedValue.rounded()))\(suffix)").font(.callout.monospacedDigit())
                .frame(width: 58, alignment: .trailing)
        }
    }
}

private struct HaloShapeIcon: InsettableShape {
    let shape: CursorHighlightSettings.Shape
    var inset: CGFloat = 0
    func path(in rect: CGRect) -> Path {
        Path(CursorHighlightRenderer.path(for: shape, in: rect.insetBy(dx: inset, dy: inset)))
    }
    func inset(by amount: CGFloat) -> Self { var copy = self; copy.inset += amount; return copy }
}
