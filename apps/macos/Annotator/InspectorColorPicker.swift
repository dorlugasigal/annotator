import AppKit
import SwiftUI
import AnnotationCore
import PresentationRendering

struct TransparencyGrid: View {
    var body: some View {
        Canvas { context, size in
            context.fill(Path(CGRect(origin: .zero, size: size)), with: .color(.white))
            let side = 6.0
            for row in 0..<Int(ceil(size.height / side)) {
                for column in 0..<Int(ceil(size.width / side)) where (row + column) % 2 == 0 {
                    context.fill(Path(CGRect(x: Double(column) * side, y: Double(row) * side, width: side, height: side)),
                                 with: .color(.gray.opacity(0.4)))
                }
            }
        }.accessibilityHidden(true)
    }
}

struct InspectorColorPicker: View {
    @ObservedObject var model: WorkspaceModel
    var fill = false
    var compact = false
    var showLabel = true
    @State private var open = false
    private var color: RGBA? { fill ? model.style.fill : model.style.color }
    var body: some View {
        let layout = compact ? AnyLayout(HStackLayout(spacing: 3)) : AnyLayout(VStackLayout(alignment: .leading, spacing: 6))
        layout {
            if showLabel {
                Text(fill ? "Fill" : compact ? "Stroke" : "Stroke color")
                    .font(.system(size: compact ? 8 : 11, weight: .medium)).foregroundStyle(.secondary)
            }
            Button { open.toggle() } label: {
                TransparencyGrid()
                    .overlay {
                        if let color {
                            PaintSwatch(color: color, gradient: fill ? model.style.fillGradient : model.style.strokeGradient)
                                .opacity(model.style.opacity)
                        }
                    }
                    .clipShape(RoundedRectangle(cornerRadius: 7))
                    .overlay(alignment: .bottomTrailing) {
                        Image(systemName: "chevron.down").font(.system(size: 7, weight: .semibold))
                            .foregroundStyle(color == nil ? Color.secondary : Color.white)
                            .padding(4)
                    }
                    .frame(width: compact ? 28 : 36, height: compact ? 28 : 36)
                    .overlay { RoundedRectangle(cornerRadius: 7).strokeBorder(Color.primary.opacity(0.14)) }
            }
            .buttonStyle(.plain)
            .onChange(of: model.workspaceHidden) { _, hidden in
                if hidden { open = false }
            }
            .onReceive(model.canvasInteraction) { open = false }
            .help(fill && color == nil ? "Transparent fill" : fill ? "Fill color" : "Stroke color")
            .accessibilityLabel(fill ? "Fill color" : "Stroke color")
            .accessibilityValue(color.map { "#\(PaletteHex.format($0))" } ?? "Transparent")
            .popover(isPresented: $open, arrowEdge: .bottom) {
                InspectorColorPalette(model: model, fill: fill, done: { open = false })
                    .onAppear { model.beginStyleEdit() }
                    .onDisappear { model.finishStyleEdit() }
            }
        }
    }
}

struct PaintSwatch: View {
    let color: RGBA
    var gradient: ColorGradient?
    var body: some View {
        if let gradient {
            GeometryReader { geometry in
                let angle = gradient.angle * .pi / 180
                let dx = cos(angle), dy = sin(angle)
                let length = (abs(dx) * geometry.size.width + abs(dy) * geometry.size.height) / 2
                let x = dx * length / max(1, geometry.size.width)
                let y = dy * length / max(1, geometry.size.height)
                LinearGradient(colors: gradient.colors.map { Color(nsColor: $0.nsColor) },
                               startPoint: UnitPoint(x: 0.5-x, y: 0.5-y),
                               endPoint: UnitPoint(x: 0.5+x, y: 0.5+y))
            }.opacity(color.alpha)
        } else { Color(nsColor: color.nsColor) }
    }
}

enum PaletteHex {
    static func format(_ color: RGBA) -> String {
        String(format: "%02X%02X%02X%02X", Int((color.red * 255).rounded()), Int((color.green * 255).rounded()),
               Int((color.blue * 255).rounded()), Int((color.alpha * 255).rounded()))
    }

    static func parse(_ draft: String) throws -> RGBA {
        var value = draft.trimmingCharacters(in: .whitespacesAndNewlines)
        if value.hasPrefix("#") { value.removeFirst() }
        guard [6, 8].contains(value.count),
              value.utf8.allSatisfy({ (48...57).contains($0) || (65...70).contains($0) || (97...102).contains($0) }),
              let code = UInt32(value, radix: 16) else {
            throw CanvasError.invalidDocument("Use six RGB or eight RGBA hex digits.")
        }
        let rgba = value.count == 6 ? (code << 8) | 255 : code
        return RGBA(Double((rgba >> 24) & 255) / 255, Double((rgba >> 16) & 255) / 255,
                    Double((rgba >> 8) & 255) / 255, Double(rgba & 255) / 255)
    }
}

struct InspectorColorPalette: View {
    @ObservedObject var model: WorkspaceModel
    let fill: Bool
    let done: () -> Void
    var availableHeight: CGFloat?
    @State private var hex = ""
    @State private var committedHex = ""
    @State private var validation: String?
    @State private var showingGradients = false
    @State private var wheelExpanded = false
    @FocusState private var editingHex: Bool
    private var selected: RGBA? { fill ? model.style.fill : model.style.color }
    private var selectedGradient: ColorGradient? { fill ? model.style.fillGradient : model.style.strokeGradient }
    private var palette: [RGBA?] {
        [fill ? nil : RGBA(0, 0, 0, 0), .ink, RGBA(0.33, 0.37, 0.39), .white, RGBA(0.53, 0.4, 0.36),
         RGBA(0.0, 0.59, 0.62), .blue, RGBA(0.49, 0.33, 0.72), RGBA(0.69, 0.32, 0.66), RGBA(0.89, 0.26, 0.4),
         RGBA(0.22, 0.62, 0.35), RGBA(0.0, 0.62, 0.51), .yellow, RGBA(0.95, 0.57, 0.13), .coral]
    }
    private var shades: [RGBA] {
        guard let base = selected else { return [] }
        return [0.5, 0.75, 1, 1.25, 1.5].map { amount in
            func component(_ value: Double) -> Double { amount <= 1 ? value * amount : value + (1 - value) * (amount - 1) }
            return RGBA(component(base.red), component(base.green), component(base.blue), base.alpha)
        }
    }
    private var maximumHeight: CGFloat {
        availableHeight
            ?? max(
                240, (model.active?.panel.screen?.visibleFrame.height ?? NSScreen.main?.visibleFrame.height ?? 800) - 80
            )
    }

    var body: some View {
        ViewThatFits(in: .vertical) {
            contents.fixedSize(horizontal: false, vertical: true)
            ScrollView { contents }.frame(height: maximumHeight)
        }
        .frame(width: 284)
        .frame(maxHeight: maximumHeight)
        .fixedSize(horizontal: false, vertical: true)
        .background(Color(nsColor: .windowBackgroundColor))
        .transaction { $0.animation = nil }
        .onAppear {
            updateHex(); showingGradients = selectedGradient != nil
        }
    }

    private var contents: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text(fill ? "Fill color" : "Stroke color").font(.system(size: 13, weight: .semibold))
                Spacer()
                if !fill && model.active?.canvas.editingShapeLabelID != nil {
                    Button("Match outline") {
                        model.matchLabelToStroke(); updateHex(); showingGradients = selectedGradient != nil
                    }
                    .controlSize(.small).disabled(model.editingLabelFollowsStroke)
                }
                if fill && selected != nil {
                    Button("Match outline") {
                        model.applyStyle(where: { $0.tool.supportsFill }) { $0.matchFillToStroke() }
                        showingGradients = selectedGradient != nil
                        updateHex()
                    }.controlSize(.small)
                }
            }.frame(minHeight: 24)
            if fill {
                Toggle(
                    "Fill shape",
                    isOn: Binding(
                        get: { selected != nil },
                        set: { enabled in
                            model.applyStyle(where: { $0.tool.supportsFill }) { $0.setFillEnabled(enabled) }
                            showingGradients = selectedGradient != nil
                            updateHex()
                        })
                ).font(.callout)
                    .help("Start with the outline's color or gradient at 40%. You can change it afterwards.")
            }
            Picker(
                "Color options",
                selection: Binding(
                    get: { showingGradients },
                    set: {
                        showingGradients = $0
                        wheelExpanded = false
                    })
            ) {
                Text("Gradient").tag(true)
                Text("Solid").tag(false)
            }.pickerStyle(.segmented).labelsHidden()
            VStack(alignment: .leading, spacing: 12) {
                if !wheelExpanded {
                    if !showingGradients {
                        LazyVGrid(columns: Array(repeating: GridItem(.fixed(38), spacing: 12), count: 5), spacing: 12) {
                            ForEach(Array(palette.enumerated()), id: \.offset) { index, color in
                                colorButton(
                                    color,
                                    name: [
                                        fill ? "None, no fill" : "Transparent stroke", "Dark ink", "Slate gray",
                                        "White", "Brown",
                                        "Teal", "Blue", "Purple", "Mauve", "Rose", "Green", "Emerald", "Yellow",
                                        "Orange", "Coral",
                                    ][index])
                            }
                        }
                        if selected != nil {
                            DisclosureGroup("Shades") {
                                HStack(spacing: 12) {
                                    ForEach(Array(shades.enumerated()), id: \.offset) { index, color in
                                        colorButton(
                                            color,
                                            name: ["Darker", "Dark", "Current", "Light", "Lighter"][index]
                                                + " shade of selected color")
                                    }
                                }.padding(.top, 8)
                            }.font(.caption)
                        }
                    } else {
                        LazyVGrid(columns: Array(repeating: GridItem(.fixed(38), spacing: 12), count: 5), spacing: 12) {
                            ForEach(GradientPreset.allCases, id: \.self) { preset in
                                Button {
                                    model.applyStyle(where: { !$0.tool.isProtection && (!fill || $0.tool.supportsFill) }
                                    ) {
                                        $0.setGradient(preset.gradient, fill: fill)
                                    }
                                    updateHex()
                                } label: {
                                    PaintSwatch(color: .white, gradient: preset.gradient)
                                        .frame(width: 38, height: 38).clipShape(RoundedRectangle(cornerRadius: 6))
                                        .padding(2)
                                        .overlay {
                                            RoundedRectangle(cornerRadius: 6).strokeBorder(
                                                selectedGradient == preset.gradient ? Color.accentColor : .clear,
                                                lineWidth: 2)
                                        }
                                }.buttonStyle(.plain).help(preset.label)
                                    .accessibilityLabel("\(preset.label) gradient")
                                    .accessibilityAddTraits(selectedGradient == preset.gradient ? .isSelected : [])
                            }
                        }
                        Text("A blend across your whole shape.")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                }
                Button {
                    wheelExpanded.toggle()
                } label: {
                    Label("Color wheel", systemImage: wheelExpanded ? "chevron.down" : "chevron.right")
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
                .font(.callout).buttonStyle(.plain)
                .accessibilityValue(wheelExpanded ? "Expanded" : "Collapsed")
                .accessibilityIdentifier("inline-color-wheel")
                if wheelExpanded {
                    InlineColorWheel(color: selected ?? model.style.color) {
                        choose($0)
                        showingGradients = false
                    }.padding(.top, 8)
                }
                if !showingGradients || wheelExpanded {
                    Text("Hex color")
                        .font(.system(size: 11)).foregroundStyle(.secondary)
                    HStack(spacing: 8) {
                        Text("#").foregroundStyle(.secondary)
                        TextField("RRGGBB or RRGGBBAA", text: $hex)
                            .font(.system(size: 12, weight: .medium, design: .monospaced))
                            .textFieldStyle(.plain).onSubmit { _ = applyHex() }
                            .focused($editingHex)
                            .accessibilityLabel(fill ? "Fill hex color and alpha" : "Stroke hex color and alpha")
                    }
                    .padding(10).background(Color.primary.opacity(0.045), in: RoundedRectangle(cornerRadius: 7))
                    .help("Six RGB or eight RGBA digits. Entering a hex color switches to solid paint.")
                }
            }
            Divider()
            HStack(spacing: 8) {
                Text(fill ? "Fill opacity" : "Stroke opacity").font(.system(size: 11))
                Slider(
                    value: Binding(
                        get: { model.style.paintOpacity(fill: fill) },
                        set: { value in
                            model.applyStyle(where: { !fill || $0.tool.supportsFill }) {
                                $0.setPaintOpacity(value, fill: fill)
                            }
                            updateHex()
                        }), in: 0...1
                )
                .accessibilityLabel(fill ? "Fill opacity" : "Stroke opacity")
                .disabled(fill && selected == nil)
                Text("\(Int((model.style.paintOpacity(fill: fill) * 100).rounded()))%")
                    .font(.system(size: 11).monospacedDigit()).frame(width: 34)
            }
            if model.style.opacity < 1 {
                HStack {
                    Text("Object opacity: \(Int(model.style.opacity * 100))%.")
                        .font(.caption).foregroundStyle(.secondary)
                    Button("Reset") { model.applyStyle { $0.opacity = 1 } }
                        .font(.caption).buttonStyle(.link)
                        .accessibilityLabel("Reset object opacity")
                        .help("Set whole-object opacity to 100%. Stroke and fill keep their own opacity.")
                }
            }
            Button {
                NSColorSampler().show { sampled in
                    Task { @MainActor in
                        guard let sampled else { return }
                        do { choose(try RGBA(sampled)) } catch { validation = error.localizedDescription }
                    }
                }
            } label: {
                Label("Pick from screen", systemImage: "eyedropper")
                    .frame(maxWidth: .infinity, minHeight: 30)
            }
            .buttonStyle(.bordered)
            .help("Choose a color anywhere on the screen. Escape cancels.")
            .accessibilityLabel("Pick from screen")
            if let validation { Text(validation).font(.caption2).foregroundStyle(.red) }
            HStack {
                Spacer()
                Button("Done") {
                    if hex == committedHex || applyHex() { done() }
                }.controlSize(.small)
            }
        }
        .padding(16)
        .frame(width: 284)
    }

    private func colorButton(_ color: RGBA?, name: String) -> some View {
        Button { choose(color, useColorAlpha: color?.alpha == 0) } label: {
            TransparencyGrid().overlay { if let color { Color(nsColor: color.nsColor) } }
                .clipShape(RoundedRectangle(cornerRadius: 6))
                .frame(width: 38, height: 38)
                .overlay { RoundedRectangle(cornerRadius: 6).strokeBorder(Color.primary.opacity(0.2)) }
                .padding(2)
                .overlay { RoundedRectangle(cornerRadius: 8).strokeBorder(
                    isSelected(color) ? Color.accentColor : .clear, lineWidth: 2) }
                .overlay {
                    if color == nil {
                        Text("None").font(.system(size: 10, weight: .medium))
                            .padding(2).background(Color(nsColor: .windowBackgroundColor))
                    }
                }
        }.buttonStyle(.plain)
            .accessibilityLabel(color.map { "\(name), hex #\(PaletteHex.format($0))" } ?? name)
            .accessibilityAddTraits(isSelected(color) ? .isSelected : [])
    }
    private func isSelected(_ value: RGBA?) -> Bool {
        guard selectedGradient == nil else { return false }
        guard let value, let selected else { return value == nil && selected == nil }
        return value.red == selected.red && value.green == selected.green && value.blue == selected.blue &&
            (value.alpha == 0 ? selected.alpha == 0 : selected.alpha > 0)
    }
    private func choose(_ value: RGBA?, useColorAlpha: Bool = false) {
        model.applyStyle(where: { !fill || $0.tool.supportsFill }) {
            $0.setPaintColor(value, fill: fill, useColorAlpha: useColorAlpha)
        }
        updateHex(); validation = nil
    }
    private func updateHex() {
        hex = selected.map(PaletteHex.format) ?? ""
        committedHex = hex
    }
    private func applyHex() -> Bool {
        do {
            let color = try PaletteHex.parse(hex)
            let digits = hex.trimmingCharacters(in: .whitespacesAndNewlines).filter { $0 != "#" }
            choose(color, useColorAlpha: digits.count == 8)
            return true
        } catch {
            validation = error.localizedDescription
            editingHex = true
            return false
        }
    }
}
